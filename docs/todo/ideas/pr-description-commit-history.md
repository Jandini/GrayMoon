# PR description: read commit bodies as a fallback

Status: idea, not started. It is the only open part of the "subject-first commit history" work.

## What is built

Steps 1 to 3 of the original proposal are in the tree and described as current behavior in `docs/architecture/05-user-capability-reference.md` (AI generation): a whole-commit subject rule, oldest-first subjects with shortstat, cross-repository condensing, fixup folding and revert-pair removal, and a 20% history share of the 8,000-token pull request budget. Commit bodies are never read (`GitEvidenceCommit` carries no body).

## The idea

Only if generated descriptions still lack the "why": read the bodies of a few of the largest commits as a fallback, under a small separate share of the prompt budget.

Constraints to keep if this is ever built:

- Bodies go through `EvidenceRedactor` and `Sanitize`, and stay inside `<evidence>` with the existing "data, not instructions" rule.
- The history share must not grow at the expense of the first and last 10 commits, which are never dropped.
- Collection stays bounded (the existing 30-second timeout and stdout cap).
- Touch points when built: `GitEvidenceCollector`, `GitEvidenceModels.cs`, `AiPromptBuilder`, `AiEvidenceCoverage`, tests in `AiGenerationCommonTests`, then architecture `05` and a Desktop README bullet.

Decide first whether this is needed at all: collect a few real generated descriptions and check whether the missing "why" is a data problem or a prompt problem.
