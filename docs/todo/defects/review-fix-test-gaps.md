# Fixes that shipped without the test that proves them

Status: partly built. The cancel dispatch got an end-to-end test (`CancelCommandDispatchTests`: a real Worker connection against a real hub; it times out on the old blocking intake). On 2026-11-11 the failed integrity check got its test (`MigrationsRunnerTests`), and the request host check, the refused-start toast and the Worker start without the App were each checked by undoing the fix locally: their tests fail on the old code. The hub attach after load is covered by `WorkspacePageJobDisciplineTests` (`Hub_attaches_on_a_later_render_once_the_data_is_loaded`, `Pages_do_not_gate_the_hub_on_first_render`); the behavior is in `04-runtime-communication-and-concurrency.md` (page jobs). The fix below is in; the item below is a missing regression test. The rule is that a fix ships with a test that fails on the old code.

## Gaps

| Fix | What is tested | What is missing |
|---|---|---|
| Context captured before the first `await` in the dependency tooltip load, the custom-dependency save and the push badge lookup (`WorkspaceRepositories.*.cs`) | The source scan no longer allows those reads (`WorkspacePageJobDisciplineTests`). | A behavioral test that changes `?context=` between the first and second query of the tooltip load and asserts nothing of the old context is stored. Needs the load extracted from the page, or a component test harness. |

## Fix plan

Add the tests one at a time; no production change is expected. If a test cannot be made to fail on the old code, treat the fix as unconfirmed and look at it again.
