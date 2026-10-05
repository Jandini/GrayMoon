namespace GrayMoon.App;

/// <summary>
/// Thrown when a strict-mode database migration step fails. The step's transaction has already been rolled
/// back, so the schema is unchanged. Startup must stop when this is thrown - see <see cref="Migrations.RunAllAsync"/>.
/// </summary>
public sealed class DatabaseMigrationException : Exception
{
    public int Version { get; }

    public string StepName { get; }

    public string? BackupPath { get; }

    public DatabaseMigrationException(int version, string stepName, string? backupPath, Exception innerException)
        : base(BuildMessage(version, stepName, backupPath), innerException)
    {
        Version = version;
        StepName = stepName;
        BackupPath = backupPath;
    }

    private static string BuildMessage(int version, string stepName, string? backupPath)
    {
        var backupNote = string.IsNullOrEmpty(backupPath)
            ? "No pre-migration backup is available."
            : $"A pre-migration backup is available at '{backupPath}'.";
        return $"Database migration step {version} ('{stepName}') failed and was rolled back. {backupNote}";
    }
}
