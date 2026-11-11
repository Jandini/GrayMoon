# Fixes that shipped without the test that proves them

Status: partly built. On 2026-11-11 the failed integrity check got its test (`MigrationsRunnerTests`), and the request host check, the refused-start toast and the Worker start without the App were each checked by undoing the fix locally: their tests fail on the old code. The fixes below are in; each item below is a missing or weaker-than-required regression test. The rule is that a fix ships with a test that fails on the old code.

## Gaps

| Fix | What is tested | What is missing |
|---|---|---|
| `CancelCommand` is not delayed by a full pool (`CommandRequestIntake` in `Hosted/SignalRConnectionHostedService.cs`) | The intake returns without waiting for queue room (`CommandRequestIntakeTests`). | That a cancel is dispatched while a queued request is waiting, with the SignalR client's one-handler-at-a-time dispatch in the picture. |
| Context captured before the first `await` in the dependency tooltip load, the custom-dependency save and the push badge lookup (`WorkspaceRepositories.*.cs`) | The source scan no longer allows those reads (`WorkspacePageJobDisciplineTests`). | A behavioral test that changes `?context=` between the first and second query of the tooltip load and asserts nothing of the old context is stored. Needs the load extracted from the page, or a component test harness. |
| Actions and Repositories hub attached after the load | Nothing. | No automated test; the manual check is "open Actions, run a Sync from another tab, the grid refreshes". |

## Fix plan

Add the tests one at a time; no production change is expected. If a test cannot be made to fail on the old code, treat the fix as unconfirmed and look at it again.
