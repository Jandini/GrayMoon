# PRAGMA synchronous=NORMAL applies only to the startup connection

## What is still open

`Program.cs` runs `PRAGMA synchronous=NORMAL` once during startup. `SqliteConnectionPragmasInterceptor` sets `busy_timeout` and `foreign_keys` on every open, and does not set `synchronous`.

SQLite does not store `synchronous` in the database file. It is per connection and defaults to `FULL`. A startup PRAGMA does not cover later pooled or factory connections.

`CODE_REVIEW.md` marks this not started (finding 7 / #7a). The interceptor comment says `synchronous` persists. That comment does not match SQLite.

## Why it matters

Factory and pooled connections still commit with `synchronous=FULL`, so each commit syncs the WAL. The review treated the move onto the interceptor as low-medium risk and not a screen change.

## Source

`CODE_REVIEW.md` (finding 7, and the status line "Not started: #7a").

## Where architecture is silent

`docs/architecture/03-data-model-and-persistence.md`, "SQLite connection setup", groups the startup `synchronous=NORMAL` with settings that are "persisted in the file or harmless to repeat". `06-developer-extension-guide.md` section 24 lists `Cache=Shared` and synchronous `EnsureCreated()`, not this pragma.
