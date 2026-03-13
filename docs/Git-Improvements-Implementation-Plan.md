## Git Improvements – Implementation Plan

This document describes how to standardize git operations in GrayMoon so that all remote git commands are executed through a single, token-aware service used by the agent and any hook-triggered flows.

---

## 1. Goals and constraints

- **Goals**
  - **G1**: Centralize all git interactions behind a **GitService** abstraction used by the agent (no direct `git` subprocess calls elsewhere).
  - **G2**: Ensure **all git commands that contact a remote** are executed with an authentication token passed via `-c` configuration.
  - **G3**: Support both:
    - **Scenario 1**: Agent operations where the app already has a token and passes it to the agent.
    - **Scenario 2**: Hook-triggered or other operations where no token is supplied; the agent must obtain it from the app via HTTP.
  - **G4**: Keep tokens short-lived and in-memory only, with clear cache invalidation when connector credentials change or become invalid.

- **Constraints and decisions**
  - **C1**: Tokens are **per-repo**, but are obtained via the repo’s **connector**:
    - `Repositories` table provides `connectorId`.
    - The connector provides the token for remote git operations.
  - **C2**: If the agent cannot obtain a valid token, or if the token is rejected as unauthorized, **remote git operations must fail hard** (no unauthenticated fallback).
  - **C3**: Tokens may be cached in-memory by the agent until:
    - The app notifies that **any connector’s token changed**, or
    - A git operation fails with **unauthorized**, in which case the relevant cached token is cleared and re-fetched once.
  - **C4**: No git submodule operations are required for now, but the implementation must be flexible enough to add them later.

---

## 2. High-level architecture

- **GitService (agent-side)**
  - Single entry point for **all** git operations executed by the agent.
  - Responsibilities:
    - Classify requested operations as **remote** (requires token) vs **local**.
    - Obtain tokens from the caller or from the app via `TokenProvider`.
    - Build and execute git commands, injecting `-c` configuration for remote operations (e.g. `-c http.extraHeader="Authorization: Basic <token>"` or equivalent).
    - Provide structured results (exit code, stdout/stderr) and non-secret logging.

- **TokenProvider (agent-side)**
  - Abstraction around token acquisition and caching.
  - Responsibilities:
    - Resolve `repoId → connectorId` (using contracts from the app).
    - Call the app’s **connector API** to obtain the token:
      - `GET /repos/{repoId}/connector -> { connectorId, token }`.
    - Cache tokens per connector (or per repo) with:
      - In-memory storage only.
      - Explicit invalidation API.
      - Automatic invalidation on unauthorized errors.

- **App Token/Connector API (app-side)**
  - Must provide the following:
    - **Connector endpoint for the agent**:
      - `GET /repos/{repoId}/connector`
      - Response: `{ connectorId, token }`
      - Token is suitable for authenticating remote git operations for that repo’s connector.
    - **Cache invalidation endpoint** (or equivalent signaling) so the app can tell the agent to clear tokens when connector credentials change (see §4.3).

---

## 3. Supported scenarios and flows

### 3.1 Scenario 1 – Direct agent calls with token

- **Use case**
  - The app initiates an agent command (e.g. sync, push, clone) and already has a token from the connector.

- **Flow**
  1. **App → Agent**
     - Request includes:
       - `repoId`
       - Operation (e.g. `push`, `fetch`, `clone`)
       - **Token** (obtained from connector via the app’s own logic).
  2. **Agent → GitService**
     - Agent calls:
       - `GitService.ExecuteRemoteAsync(repoId, gitArgs, tokenFromCaller)`
       - Or a strongly-typed method such as `PushAsync`, `FetchAsync`, `CloneAsync`, etc.
  3. **GitService**
     - Recognizes a remote operation and uses the **provided token** directly (skips TokenProvider).
     - Builds git command with `-c` configuration to add the Authorization header.
     - Executes the git subprocess and returns the result.
  4. **Error behavior**
     - If git returns **unauthorized** for a provided token:
       - GitService surfaces the failure **without** retrying or re-fetching a token (the app is responsible for providing a fresh token).

### 3.2 Scenario 2 – Hook-triggered / no token provided

- **Use case**
  - A git hook fires (e.g. pre-push, post-receive) and calls into the agent, but does **not** have a token available.

- **Flow**
  1. **Hook → Agent**
     - Hook sends:
       - `repoId` (preferred), or enough information to resolve it (e.g. repo path mapped in the app/agent).
       - Hook context (e.g. `hookType`, refspecs).
  2. **Agent → GitService**
     - Agent calls:
       - `GitService.ExecuteRemoteAsync(repoId, gitArgs, token: null)`.
  3. **GitService → TokenProvider**
     - Sees that this is a **remote** operation with no token.
     - Calls `TokenProvider.GetTokenForRepoAsync(repoId)`.
  4. **TokenProvider → App**
     - On cache miss:
       - Calls `GET /repos/{repoId}/connector`.
       - Receives `{ connectorId, token }`.
       - Caches the token keyed by connectorId (or repoId).
  5. **GitService**
     - Builds git command with `-c` configuration using the obtained token.
     - Executes git and returns result.
  6. **Error behavior**
     - If git returns **unauthorized**:
       - TokenProvider invalidates the cached token for that connector.
       - Optionally re-fetches a fresh token once and re-runs the git command.
       - If the second attempt is still unauthorized, the operation **fails hard** and the error is surfaced back to the hook/app.

---

## 4. Token caching and invalidation

### 4.1 Caching model

- **Cache scope**
  - Cache tokens **per connector** (recommended) or per repo:
    - Key: `connectorId` (or `repoId` if simpler).
    - Value: `{ token, lastFetchedAt }`.
  - Use process-local, in-memory storage only (no persistence).

- **Lifetime**
  - Tokens remain cached until one of the following:
    - **App-initiated invalidation** for a connector (e.g. connector updated in the UI/admin).
    - **Unauthorized** response from git when using that token (TokenProvider clears and re-fetches once).

### 4.2 App-initiated invalidation

- The app should provide a way to notify all relevant agents when connector credentials change, for example:
  - `POST /agents/tokens/invalidate`
  - Body: `{ connectorId }`
- Agent behavior:
  - TokenProvider receives invalidation request and removes cached entry for `connectorId`.
  - Next remote operation for any repo using that connector will trigger a fresh call to `GET /repos/{repoId}/connector`.

### 4.3 Unauthorized-based invalidation

- When a remote git operation executed with a cached token returns **unauthorized**:
  - TokenProvider clears the cache entry for that connector.
  - Tries to fetch a new token exactly once via `GET /repos/{repoId}/connector`.
  - If git is still unauthorized with the new token:
    - Operation fails hard and the error is propagated to the caller.

---

## 5. Remote operations coverage

### 5.1 Required behavior

- **All git commands that can contact a remote must be executed with a token and `-c` configuration.**
- At minimum, GitService must handle these as **remote** operations:
  - `git fetch`
  - `git fetch origin`
  - `git fetch origin <branch>`
  - `git pull`
  - `git pull origin <branch>`
  - `git push`
  - `git push origin <branch>`
  - `git clone`
  - `git ls-remote`
  - `git ls-remote <remote>`
  - `git remote show <remote>`
  - (Submodules: not required now, but should be easy to add later.)

### 5.2 Codebase analysis checklist

- As part of implementation, perform a code search to ensure **all remote git operations are routed through GitService**:
  - **Step 1**: Identify current git invocation points:
    - Search for `"git "` invocations in:
      - Agent command handlers.
      - Any app-side helpers that might run git directly.
  - **Step 2**: Classify each invocation as:
    - **Remote** (requires token and `-c`).
    - **Local-only** (e.g. `git status`, `git diff` on local repo).
  - **Step 3**: Refactor:
    - Replace each remote invocation with a call to GitService.
    - Ensure the call site either:
      - Provides a token (Scenario 1), or
      - Provides `repoId` so TokenProvider can resolve the token (Scenario 2).
  - **Step 4**: Add tests or diagnostics to verify that:
    - Any attempt to run a remote operation **without a token** is rejected by GitService.

---

## 6. Implementation steps

### 6.1 App-side changes

- **Endpoint: `GET /repos/{repoId}/connector`**
  - Request:
    - `repoId` path parameter.
  - Response:
    - `{ connectorId, token }`
    - `token` is a connector-scoped credential the agent can use for remote git.
  - Behavior:
    - Validate that the caller (agent) is authorized to obtain a token for the repo.
    - Resolve `connectorId` from `Repositories` table.
    - Obtain the connector’s token and return it.

- **Optional endpoint: `POST /agents/tokens/invalidate`**
  - Request body: `{ connectorId }`
  - Behavior:
    - Notify one or more agents (e.g., via HTTP or message bus) to clear cached tokens for that connector.

### 6.2 Agent-side changes

- **Introduce GitService**
  - Create an abstraction (e.g. `IGitService`) with methods aligned to core operations:
    - `Task ExecuteRemoteAsync(Guid repoId, string[] gitArgs, string? tokenFromCaller = null)`
    - Optional typed methods: `CloneAsync`, `FetchAsync`, `PullAsync`, `PushAsync`, `LsRemoteAsync`, etc.
  - Implement classification logic:
    - Given `gitArgs`, determine if the operation is remote and requires a token.
  - Implement process execution:
    - For remote operations:
      - Ensure a token is available (via caller or TokenProvider).
      - Inject token into git via `-c` configuration.
    - For local operations:
      - Execute without token.

- **Introduce TokenProvider**
  - Define an interface (e.g. `ITokenProvider`) with methods:
    - `Task<string> GetTokenForRepoAsync(Guid repoId)`
    - `void InvalidateByConnectorId(Guid connectorId)`
  - Implement:
    - In-memory cache keyed by connectorId.
    - HTTP client for `GET /repos/{repoId}/connector`.
    - Unauthorized-based invalidation behavior.

- **Wire up hooks**
  - Update hook handling so that:
    - Hook payloads always include `repoId` (or enough info to resolve to `repoId`).
    - Hook-triggered operations call GitService rather than running git directly.

### 6.3 Migration and hardening

- **Refactor existing code**
  - Replace all direct git subprocess calls with GitService calls (especially in agent commands).
  - Ensure that:
    - App-initiated operations pass tokens when they already have them.
    - Hook-initiated operations provide `repoId` and rely on TokenProvider.

- **Add logging and metrics**
  - Log (without secrets):
    - Which operations are treated as remote vs local.
    - Cache hits/misses for tokens (per connector).
    - Unauthorized failures and subsequent retries.
  - Add metrics where appropriate (e.g. token fetch count, cache hit ratio, git command latency).

---

## 7. Verification checklist

- **App-side**
  - `GET /repos/{repoId}/connector` implemented and tested.
  - (Optional) token invalidation endpoint for connectors implemented and wired to connector update events.

- **Agent-side**
  - GitService exists and is the **only** place that runs git for agent operations.
  - TokenProvider correctly fetches and caches tokens per connector using the app API.
  - Remote operations:
    - Always require a token.
    - Always inject a token via `-c` configuration.
    - Fail hard if token is missing or unauthorized.

- **Codebase**
  - All existing remote git operations are routed through GitService.
  - New operations have clear guidance to use GitService and specify whether they are remote or local.

