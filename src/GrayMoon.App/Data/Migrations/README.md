# Migrations

GrayMoon does not use EF Core generated migrations, and nothing in this folder is executed.

- Brand-new databases are created from the EF model by `EnsureCreated()` in `Program.cs`.
- Existing databases are patched at startup by guarded, idempotent methods in `src/GrayMoon.App/Migrations.cs` and `src/GrayMoon.App/Migrations.Features.cs`, called in order from `Migrations.RunAllAsync`.

Every schema change needs both an `AppDbContext` model update and a new guarded `Migrate*Async` method. See "Database schema" in the root `CLAUDE.md`.
