# Migrations

GrayMoon does not use EF Core generated migrations, and nothing in this folder is executed.

- Brand-new databases are created from the EF model by `EnsureCreated()` in `Program.cs`.
- `Migrations.RunAllAsync` (`src/GrayMoon.App/Migrations.cs`, `Migrations.Features.cs`) then runs every numbered step above the database's SQLite `PRAGMA user_version`. Step 1 is the tolerant legacy baseline (`RunLegacyBaselineAsync`). Steps 2+ are the ordered `StrictSteps` list; each runs in its own transaction that also sets `user_version`, and a failure rolls back and stops startup with `DatabaseMigrationException`.
- Before any pending step runs, the database is backed up with `VACUUM INTO` to `graymoon.db.bak-<yyyyMMdd-HHmmss>` next to it; the 3 newest backups are kept.
- A new database starts at `user_version` 0, so every step also runs against a freshly created schema and must be idempotent.

To add a schema change:

1. Update the entity and its `OnModelCreating` configuration in `AppDbContext` (Feature context tables: `AppDbContext.Features.cs`). New columns must be nullable or have a default.
2. Add a guarded, idempotent `public static async Task MigrateXxxAsync(AppDbContext dbContext, ILogger? logger = null)` to `Migrations.cs` (Feature context tables: `Migrations.Features.cs`), using `pragma_table_info` / `sqlite_master` checks or helpers such as `AddNullableTextColumnIfMissingAsync`. No try/catch: let failures roll the step back.
3. Append `(next version, "Short name", dbContext => MigrateXxxAsync(dbContext))` to `StrictSteps`. Never edit, renumber or delete a shipped step, and never add to the legacy baseline.

See "Database schema" in the repository-root `CLAUDE.md` and `docs/architecture/03-data-model-and-persistence.md`.
