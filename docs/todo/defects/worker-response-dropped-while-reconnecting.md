# A Worker command result produced during a reconnect is dropped

Status: not built. Traced in the code, not reproduced; confirm with a failing test before fixing.

## Symptom

A command that finishes on the Worker while the hub connection is reconnecting looks hung in the App for `CommandTimeoutSeconds` (240 s), then fails with a timeout although the work was done.

## Cause

`SignalRConnectionHostedService.SendCommandResponseAsync` (`src/GrayMoon.Worker/Hosted/SignalRConnectionHostedService.cs`) returns a completed task when the connection state is not `Connected`. The response is discarded without a log line, and nothing sends it later.

## Fix plan

Pick one:

- Keep undelivered responses in a small bounded queue and send them from the `Reconnected` handler and after a successful reconnect loop. The App must still accept a response for a request it is waiting on; check `WorkerResponseDelivery` for requests failed by the disconnect (the App fails the commands routed to a dropped connection id, so a late response may have no waiter).
- Or log a warning with the request id and command, and let the App fail the request at once on disconnect instead of waiting for the timeout. Check what the App does today before choosing; if it already fails the request on disconnect, only the Worker log line is missing.

## Tests

A hosted-service test that produces a response while the connection is not connected and asserts the chosen outcome (resent after reconnect, or logged).

## Rule this falls under

`AGENTS.md`, "Failures, cancellation and waiting", rule 1: never discard a Worker response.
