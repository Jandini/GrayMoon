# Worker startup does not report a damaged install once

## What is still open

There is no Worker startup probe that reports a damaged install and asks for one reinstall. After a torn extract, a failed type initializer stays cached in that Worker process, and later repository calls fail one by one instead of failing once with a reinstall message.

No App or Worker source contains that probe.

The same note's other follow-up, not sending a second install while one is running, is already in the architecture docs. This item is only the probe.

## Why it matters

The investigation's user-visible failure was Remove Feature (and Sync) failing per repository after a half-written Worker, until the service was restarted and a full install was run.

## Source

`GrayMoon/docs/worker-install/GrayMoon-Worker-Install-Race-Investigation.md` section 6, the "Not done" follow-up.

## Where architecture is silent

Checked `docs/architecture/02-system-architecture.md` section 17, `04-runtime-communication-and-concurrency.md` section 21, and `GrayMoon.Desktop/docs/architecture/04-developer-extension-guide.md` section 18. Those record the missing App/Desktop install single-flight and the install-script mutex. They do not mention a damaged-install probe.
