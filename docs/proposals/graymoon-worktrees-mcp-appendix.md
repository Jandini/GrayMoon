# Appendix — MCP-Controlled Worktrees and AI Agent Orchestration

This appendix extends the GrayMoon worktree design with an MCP layer that allows AI clients and coding agents to safely inspect and control GrayMoon-managed development environments.

The core idea is:

> **GrayMoon becomes the controlled orchestration layer, and MCP becomes the interface AI agents use to operate it.**

This fits the existing GrayMoon model well because GM already understands:

- workspaces
- repositories
- branches
- Git state
- staged and unstaged changes
- pull requests
- GitHub Actions
- dependency relationships
- remote agents
- multi-repository changes

MCP should expose these capabilities to AI clients without requiring those clients to directly understand GrayMoon internals or directly shell into developer machines.

---

## 1. Architectural Principle

The MCP server should not become a second Git implementation.

It should act as another front door into GrayMoon application services.

```text
Cursor / Claude / Codex / MCP Client
                  │
                  │ MCP
                  ▼
          GrayMoon MCP Server
                  │
                  ▼
        GrayMoon Application Services
          │        │        │
          │        │        └─ GitHub / PR / CI
          │        └────────── Workspace State
          └─────────────────── Agent Commands
                                   │
                                   ▼
                             GrayMoon Agent
                                   │
                                   ▼
                        Git / Filesystem / Build
```

The important rule is:

```text
MCP
  ↓
GrayMoon services
  ↓
GrayMoon agent
  ↓
Git / OS
```

not:

```text
MCP
  ↓
git.exe
```

This keeps all policy, permissions, validation, state tracking, logging, and multi-repository orchestration inside GrayMoon.

---

## 2. Working Context as the Core Abstraction

The worktree design should introduce a general concept of a **Working Context**.

A Working Context represents the physical repository locations against which GrayMoon operations execute.

```text
Workspace
    │
    ├── Repositories
    │
    └── Working Contexts
           │
           ├── Primary
           ├── feature/search-v2
           ├── fix/export-timeout
           ├── codex-a81d20
           └── claude-8812
```

Suggested types:

```text
WorkingContextType
------------------
Primary
Worktree
Agent
Temporary
```

A normal human-created workspace worktree would typically use:

```text
Type = Worktree
```

An AI-created isolated worktree could use:

```text
Type = Agent
```

The underlying implementation can still use Git worktrees for both.

The advantage is that the rest of GrayMoon does not need to care whether the context originated from a user, an AI agent, or an automation.

---

## 3. Repository Path Resolution

Operations should resolve:

```text
WorkspaceId
+
RepositoryId
+
WorkingContextId
```

into:

```text
PhysicalRepositoryPath
```

For example:

```text
Workspace: AuroraReview
Repository: aurorareview-api
Working Context: codex-a81d20
```

might resolve to:

```text
C:\GrayMoonWorkspaces\AuroraReview\
    worktrees\
        codex-a81d20\
            aurorareview-api\
```

The existing Git services should continue operating against a normal repository path.

Suggested abstraction:

```csharp
public interface IRepositoryPathResolver
{
    string GetRepositoryPath(
        WorkspaceId workspaceId,
        RepositoryId repositoryId,
        WorkingContextId? workingContextId);
}
```

This prevents worktree awareness from spreading through every Git service.

Existing operations can remain conceptually simple:

```text
GetStatus(repositoryPath)
GetDiff(repositoryPath)
Stage(repositoryPath)
Unstage(repositoryPath)
Commit(repositoryPath)
RunBuild(repositoryPath)
```

---

## 4. MCP Session to Working Context

An MCP client should be able to work inside an isolated context.

Conceptually:

```text
MCP Session
    │
    └── WorkingContextId
            │
            ├── common -> worktree path
            ├── api    -> worktree path
            └── web    -> worktree path
```

The client does not need to repeatedly pass physical paths.

Instead it references GrayMoon identities:

```text
workingContextId = wc_41d92
repositoryId     = aurorareview-api
```

GrayMoon resolves everything else.

This is safer and allows physical paths to remain an implementation detail.

---

## 5. Agent Worktree Creation

One of the first MCP capabilities should be creation of an isolated working context.

Conceptually:

```text
create_working_context(
    workspace,
    name,
    repositories,
    base_branch
)
```

Example response:

```json
{
  "workingContextId": "wc_41d92",
  "name": "codex-41d92",
  "type": "Agent",
  "repositories": [
    {
      "name": "aurorareview-api",
      "branch": "feature/metadata-filter",
      "path": "..."
    },
    {
      "name": "aurorareview-web",
      "branch": "feature/metadata-filter",
      "path": "..."
    }
  ]
}
```

GrayMoon internally handles:

```text
repository validation
fetch
branch validation
branch creation
worktree creation
path registration
working context persistence
status refresh
```

The MCP client receives a clean working context rather than having to orchestrate these low-level Git steps.

---

## 6. Example AI Workflow

A high-level user request could be:

```text
Implement metadata filtering in API and Web.
```

An MCP-enabled AI client could perform:

```text
1. Find the correct GrayMoon workspace.

2. Inspect repository state.

3. Create an isolated working context:
   feature/metadata-filter

4. Add:
   - aurorareview-api
   - aurorareview-web

5. Inspect existing branch and dependency state.

6. Edit code within the isolated worktree.

7. Ask GrayMoon for Git status.

8. Ask GrayMoon for diffs.

9. Run builds and tests.

10. Stage the required changes.

11. Generate commit messages.

12. Commit.

13. Push.

14. Create pull requests.

15. Inspect CI status.

16. Clean up the working context after merge.
```

The primary workspace remains untouched during the entire operation.

---

## 7. Why Worktrees Matter for AI

Without worktrees, AI coding agents frequently operate directly against the user's current checkout.

That creates several problems:

```text
AI changes files while user is working
branch switches affect both
staging state becomes shared
agents interfere with each other
temporary experiments pollute the main checkout
```

With GM-managed worktrees:

```text
Primary workspace
    untouched

worktrees/
    codex-41d92/
        api/
        web/

    claude-f9331/
        common/
        api/
```

Multiple independent agents can work simultaneously.

```text
                  Main Workspace
                        │
           ┌────────────┼────────────┐
           │            │            │
       Codex #1      Claude #1      User
           │            │            │
      Worktree A    Worktree B    Primary
```

Each agent gets:

- independent files
- independent branch
- independent index/staging state
- independent uncommitted changes

while still sharing the original Git object database.

---

## 8. Suggested MCP Tool Surface

GrayMoon should expose both low-level and high-level tools.

### Workspace

```text
workspace_list
workspace_get
workspace_get_status
workspace_get_change_impact
```

### Repository

```text
repository_list
repository_get
repository_get_status
repository_get_branches
```

### Working Context / Worktree

```text
working_context_list
working_context_get
working_context_create
working_context_add_repository
working_context_remove_repository
working_context_delete
working_context_get_status
```

Possible aliases may expose worktree terminology directly:

```text
worktree_list
worktree_create
worktree_remove
```

but internally GrayMoon should use the broader Working Context model.

### Git Changes

```text
git_get_status
git_get_changes
git_get_diff
git_stage
git_unstage
git_commit
```

### Branches

```text
branch_create
branch_checkout
branch_compare
branch_delete
```

### Pull Requests

```text
pull_request_list
pull_request_create
pull_request_get_status
```

### GitHub Actions

```text
actions_get_runs
actions_get_run
actions_run_workflow
```

### Build and Test

```text
build_run
test_run
```

These may execute through the GrayMoon Agent.

---

## 9. Prefer High-Level MCP Operations

GrayMoon should not force AI clients to perform dozens of low-level calls when GM already understands the workflow.

For example, expose:

```text
workspace_create_feature
```

instead of requiring the AI to manually:

```text
fetch
check branch
create branch
create worktree
register path
refresh status
```

Example:

```text
workspace_create_feature(
    workspace: "AuroraReview",
    branch: "feature/search-v2",
    repositories: [
        "aurorareview-common",
        "aurorareview-api",
        "aurorareview-web"
    ]
)
```

Similarly useful high-level operations:

```text
workspace_commit_changes
workspace_push_feature
workspace_create_pull_requests
workspace_cleanup_feature
```

Low-level tools should still exist for advanced clients, but GrayMoon's real value is orchestration.

---

## 10. MCP Resources

In addition to executable tools, GrayMoon could expose MCP resources.

For example:

```text
graymoon://workspace/AuroraReview
graymoon://workspace/AuroraReview/repositories
graymoon://workspace/AuroraReview/working-contexts

graymoon://working-context/wc_41d92
graymoon://working-context/wc_41d92/status

graymoon://repository/aurorareview-api/status
graymoon://repository/aurorareview-api/diff
```

Resources are useful when the AI needs to inspect state rather than perform an action.

This cleanly separates:

```text
read state
```

from:

```text
perform operation
```

---

## 11. Permission Model

GrayMoon should not expose unrestricted "AI controls everything" access.

MCP access should be permission-based.

Example:

```text
MCP Permissions

Read workspace state       ✓
Read repository state      ✓
Read diffs                 ✓

Create worktrees           ✓
Modify staging             ✓
Create commits             ✓

Push branches              ✓
Create pull requests       ✓

Merge pull requests        ✗
Delete branches            ✗
Force push                 ✗
Run deployment workflows   ✗
```

Suggested profiles:

```text
Read Only
Developer
PR Automation
Full Control
Custom
```

---

## 12. Approval Levels

MCP actions should also have risk classifications.

### Safe

```text
status
diff
branch inspection
workspace inspection
CI status
```

### Write

```text
create worktree
create branch
stage
unstage
commit
```

### External

```text
push
create PR
run workflow
publish package
```

### Destructive

```text
reset --hard
delete branch
force push
delete dirty worktree
delete remote branch
```

GrayMoon can require approval for higher-risk operations.

Conceptually an MCP response could indicate:

```text
ApprovalRequired
```

with:

```text
operation
risk level
affected repositories
reason
```

This is significantly safer than providing unrestricted shell access to an AI agent.

---

## 13. Remote Workspace Control

Because GrayMoon already uses a remote agent model, the MCP server does not necessarily require direct filesystem access.

The architecture can remain:

```text
MCP Client
     │
     ▼
GrayMoon Server
     │
 SignalR / Command Queue
     │
     ▼
GrayMoon Agent
     │
     ▼
Git / Filesystem / Build Tools
```

This allows an AI client to operate development environments on remote machines without receiving SSH, RDP, or direct machine access.

Example:

```text
Workspace: AuroraReview
Machine: DEV-PC-01

Workspace: CustomerA
Machine: BUILD-02

Workspace: Legacy
Machine: WIN-SERVER-17
```

The AI talks only to GrayMoon.

GrayMoon controls:

```text
authentication
authorization
workspace access
allowed tools
agent routing
logging
approval
execution
```

This could position GrayMoon as an:

> **MCP gateway to managed development environments.**

---

## 14. Workspace Intelligence Through MCP

A generic Git MCP server only understands Git.

GrayMoon can expose richer information.

For example:

```text
workspace_get_change_impact
```

could return:

```text
Changing aurorareview-common may affect:

aurorareview-api
    references Common 4.18.0

aurorareview-ingestion
    references Common 4.18.0

aurorareview-web
    depends indirectly through generated API client
```

Other GrayMoon-specific intelligence could include:

```text
affected repositories
dependency order
package update requirements
open PR relationships
CI state
branch divergence
workspace synchronization state
repository ownership
deployment relationships
```

This is where GrayMoon becomes much more valuable than a generic Git MCP implementation.

---

## 15. Suggested Server Structure

A dedicated project could be introduced:

```text
GrayMoon.Mcp
```

Possible structure:

```text
GrayMoon.Mcp
│
├── McpServer
│
├── Tools
│   ├── WorkspaceTools
│   ├── RepositoryTools
│   ├── WorkingContextTools
│   ├── GitTools
│   ├── PullRequestTools
│   ├── ActionsTools
│   └── BuildTools
│
└── Resources
    ├── WorkspaceResources
    ├── RepositoryResources
    └── WorkingContextResources
```

The MCP layer should call interfaces such as:

```text
IWorkspaceService
IWorkingContextService
IRepositoryPathResolver
IGitChangesService
IWorktreeService
IPullRequestService
IActionsService
IBuildService
```

It should not directly call:

```text
EF DbContext
git.exe
GitHub REST client
SignalR hub
```

This keeps the MCP adapter thin and reusable.

---

## 16. Recommended Delivery Stages

### V1 — Read-Only MCP

Expose:

```text
workspace_list
workspace_get_status
repository_list
repository_get_status
git_get_changes
git_get_diff
working_context_list
```

Goal:

> Allow AI clients to understand GrayMoon state safely.

---

### V2 — Worktree / Working Context Control

Expose:

```text
working_context_create
working_context_add_repository
working_context_remove_repository
working_context_delete
branch_create
```

Goal:

> Allow AI clients to create isolated development environments.

---

### V3 — Git Write Operations

Expose:

```text
git_stage
git_unstage
git_commit
```

Goal:

> Allow AI clients to prepare changes while remaining inside GM-managed contexts.

---

### V4 — Remote Collaboration Operations

Expose:

```text
push
pull_request_create
actions_run_workflow
```

with permission and approval controls.

Goal:

> Allow an AI task to reach the PR/CI stage.

---

### V5 — Workspace-Level AI Orchestration

Expose high-level operations such as:

```text
workspace_create_feature
workspace_commit_changes
workspace_push_feature
workspace_create_pull_requests
workspace_cleanup_feature
```

Goal:

> Let agents reason about and execute distributed changes across many repositories.

---

## 17. Strategic Positioning

Without MCP, GrayMoon is primarily:

> A multi-repository Git and development orchestration tool.

With MCP + managed worktrees, it can become:

> **A safe development workspace orchestration platform for both humans and AI agents.**

Or more specifically:

> **GrayMoon gives humans and AI agents isolated, controlled, multi-repository development environments through one orchestration layer.**

That direction is differentiated from:

```text
Git clients
GitHub
generic MCP Git servers
IDE-integrated coding agents
plain shell-based agents
```

because GrayMoon understands the workspace as a distributed system rather than as an individual repository.

---

## 18. Recommended Core Design Decision

The most important design decision is to make this abstraction first-class:

```text
Workspace
    +
Repository
    +
Working Context
        ↓
Physical Repository Path
```

Once this exists, the same context can be used consistently by:

```text
Git Changes
Diff
Stage / Unstage
Commit
Build
Test
Package
Push
Pull Request creation
CI
AI agents
MCP
Cleanup
```

MCP then becomes another consumer of the same GrayMoon services rather than a separate automation subsystem.

That is the architecture I would build toward.
