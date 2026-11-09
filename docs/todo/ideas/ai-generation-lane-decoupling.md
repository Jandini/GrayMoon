# AI generation vs mutation-lane decoupling

Status: **built.** The dedicated Worker AI pool, the Git Changes AI control gate and the overlap suggestion are current behavior in `04-runtime-communication-and-concurrency.md` (AI generation commands, Worker job pools), `06-developer-extension-guide.md` (pool table), `02-system-architecture.md` (hosted services) and `GrayMoon/CLAUDE.md`. This file keeps only what was not built.

## Not built

Gating the New Pull Request AI button separately from the dialog's `IsBusy`. `NewPullRequestModal` takes `IsBusy` from Repositories (`IsJobRunning`) and disables every control in the dialog, including Close, the branch pickers, the fields and both Create buttons. The dialog lives on Repositories, where the overlay already covers the page for a job started there. Enabling only the AI button would let generated text rewrite those fields while a Create job is running, so the button stays tied to `IsBusy`. The Worker AI pool still serves that dialog's generation.
