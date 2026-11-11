# Worker startup does not report a damaged install once

Status: not built. Deferred on 2026-11-11 (Optional): the only known cause is fixed and the failure has not been seen since.

## What is still open

There is no Worker startup probe that reports a damaged install and asks for one reinstall. After a torn extract, a failed type initializer stays cached in that Worker process, and later repository calls fail one by one instead of failing once with a reinstall message.

No App or Worker source contains that probe.

## Background

This happened once, on 2026-10-17. Two Worker installs ran at the same time (the automatic self-update after a branch switch, and a click on Install Worker eight seconds later). One deleted files the other had just extracted, so the Worker started without `git2-5853918.dll` and every repository call (Remove Feature, Sync) failed with "The type initializer for 'LibGit2Sharp.Core.NativeMethods' threw an exception" until a clean reinstall. No data was lost.

The cause was fixed in the same change (#462): the install script holds a machine-wide mutex, uses a unique zip name per run, and verifies every archive entry before installing. The probe was that investigation's optional follow-up, for a damaged install from any other cause. The investigation document was removed in #463; it is in git history at `e4385f4a:docs/worker-install/`.

## Sketch, if it is built

At Worker start, before connecting, touch the types whose initializers failed in the investigation (the LibGit2Sharp native load is the likely one) inside one try/catch. On failure, report one state through `GetHostInfo` and the connection tracker, so the App shows a single "Worker install is damaged, reinstall" message and refuses commands like `VersionMismatch` does. Do not add a per-feature capability flag. Test with an injected failing probe. Decide the reporting channel before building; it needs a small design.

## Where architecture is silent

`docs/architecture/02-system-architecture.md`, `04-runtime-communication-and-concurrency.md` and `GrayMoon.Desktop/docs/architecture/04-developer-extension-guide.md` record the install single-flight and the install-script mutex. They do not mention a damaged-install probe.
